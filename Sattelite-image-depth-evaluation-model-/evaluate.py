import os
import numpy as np
import tifffile
import matplotlib.pyplot as plt

def resolve_path(user_input, default_dir="dataset"):
    """Automatically prepends the dataset/ directory if needed."""
    # If the user typed an absolute path or already included the folder name, leave it alone
    if os.path.isabs(user_input) or user_input.startswith(default_dir):
        return user_input
    
    # Otherwise, assume the file is in the dataset directory
    return os.path.join(default_dir, user_input)

def evaluate_model(pred_path, truth_path, output_path):
    print(f"\nLoading predicted DSM: {pred_path}")
    print(f"Loading ground truth:  {truth_path}")
    
    y_pred = tifffile.imread(pred_path).astype(np.float32)
    y_true = tifffile.imread(truth_path).astype(np.float32)
    
    # 1. Shape Validation
    if y_pred.shape != y_true.shape:
        raise ValueError(f"Shape mismatch! Predicted: {y_pred.shape}, Truth: {y_true.shape}")
        
    # 2. Handle 'NoData' values in Ground Truth
    valid_mask = np.isfinite(y_true) & (y_true > -500)
    
    y_pred_valid = y_pred[valid_mask]
    y_true_valid = y_true[valid_mask]
    
    # 3. Calculate Errors
    print("Calculating error metrics...")
    error = y_pred_valid - y_true_valid
    
    mae = np.mean(np.abs(error))
    rmse = np.sqrt(np.mean(error**2))
    
    print("\n" + "="*35)
    print("📊 EVALUATION RESULTS")
    print("="*35)
    print(f"Mean Absolute Error (MAE):     {mae:.2f} meters")
    print(f"Root Mean Square Error (RMSE):    {rmse:.2f} meters")
    print("="*35 + "\n")
    
    # 4. Generate the Error Heatmap
    print("Generating Error Heatmap...")
    error_2d = np.zeros_like(y_true)
    error_2d[valid_mask] = y_pred_valid - y_true_valid
    
    vmax = 10.0 
    
    plt.figure(figsize=(10, 8))
    plt.imshow(error_2d, cmap='coolwarm', vmin=-vmax, vmax=vmax)
    
    cbar = plt.colorbar()
    cbar.set_label('Error in Meters (Predicted - Truth)')
    
    plt.title(f"Model Error Heatmap\nRMSE: {rmse:.2f}m | MAE: {mae:.2f}m")
    plt.axis('off')
    plt.tight_layout()
    plt.savefig(output_path, dpi=300)
    print(f"Saved visualization to '{output_path}'.")

if __name__ == '__main__':
    print("=== DSM Evaluation Tool ===")
    print("(Assuming files are in the 'dataset/' directory)\n")
    
    # Get Predicted Path
    raw_pred = input("Enter predicted DSM filename: ").strip().strip("'\"")
    pred_path = resolve_path(raw_pred)
    while not os.path.exists(pred_path):
        print(f"File not found: {pred_path}")
        raw_pred = input("Please enter a valid predicted DSM filename: ").strip().strip("'\"")
        pred_path = resolve_path(raw_pred)
        
    # Get Ground Truth Path
    raw_truth = input("Enter ground truth DSM filename: ").strip().strip("'\"")
    truth_path = resolve_path(raw_truth)
    while not os.path.exists(truth_path):
        print(f"File not found: {truth_path}")
        raw_truth = input("Please enter a valid ground truth filename: ").strip().strip("'\"")
        truth_path = resolve_path(raw_truth)
        
    # Get Output Path
    raw_output = input("Enter output heatmap filename [default: evaluation_heatmap.png]: ").strip().strip("'\"")
    if not raw_output:
        output_path = resolve_path("evaluation_heatmap.png")
    else:
        output_path = resolve_path(raw_output)
        
    evaluate_model(pred_path, truth_path, output_path)